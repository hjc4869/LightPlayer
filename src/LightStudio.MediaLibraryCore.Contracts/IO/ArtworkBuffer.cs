#nullable enable
using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.IO;

/// <summary>
/// Encoded artwork held in native memory, so loading many covers does not churn the managed heap.
/// Disposing the buffer frees the memory immediately.
/// </summary>
public sealed class ArtworkBuffer : IDisposable
{
    private nint address;

    private unsafe ArtworkBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        address = (nint)NativeMemory.Alloc((nuint)length);
        Length = length;
    }

    ~ArtworkBuffer() => Free();

    public int Length { get; }

    /// <summary>The bytes. Valid until the buffer is disposed.</summary>
    public unsafe Span<byte> Span => address != 0
        ? new Span<byte>((void*)address, Length)
        : throw new ObjectDisposedException(nameof(ArtworkBuffer));

    /// <summary>The bytes as memory. Valid until the buffer is disposed.</summary>
    public Memory<byte> Memory => address != 0
        ? new NativeMemoryManager(address, Length).Memory
        : throw new ObjectDisposedException(nameof(ArtworkBuffer));

    /// <summary>Allocates an uninitialized buffer for the caller to fill through <see cref="Span"/>.</summary>
    public static ArtworkBuffer Allocate(int length) => new(length);

    public static ArtworkBuffer Copy(ReadOnlySpan<byte> content)
    {
        var buffer = new ArtworkBuffer(content.Length);
        content.CopyTo(buffer.Span);
        return buffer;
    }

    /// <summary>Reads the first <paramref name="length"/> bytes of a stream; returns null when empty.</summary>
    public static ArtworkBuffer? Read(Stream stream, long length)
    {
        if (length <= 0 || length > Array.MaxLength)
            return null;

        var buffer = new ArtworkBuffer((int)length);
        try
        {
            stream.ReadExactly(buffer.Span);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <inheritdoc cref="Read(Stream, long)"/>
    public static async Task<ArtworkBuffer?> ReadAsync(Stream stream, long length, CancellationToken cancellationToken)
    {
        if (length <= 0 || length > Array.MaxLength)
            return null;

        var buffer = new ArtworkBuffer((int)length);
        try
        {
            using var memory = new NativeMemoryManager(buffer.address, buffer.Length);
            await stream.ReadExactlyAsync(memory.Memory, cancellationToken).ConfigureAwait(false);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    private unsafe void Free()
    {
        nint released = Interlocked.Exchange(ref address, 0);
        if (released != 0)
            NativeMemory.Free((void*)released);
    }

    private sealed unsafe class NativeMemoryManager(nint address, int length) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan() => new((void*)address, length);

        public override MemoryHandle Pin(int elementIndex = 0) => new((byte*)address + elementIndex);

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
