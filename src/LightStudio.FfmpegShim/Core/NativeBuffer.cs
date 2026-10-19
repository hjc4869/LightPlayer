using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace LightStudio.FfmpegShim.Core;

/// <summary>Contiguous native bytes owned until disposal. Views must not outlive their owner.</summary>
public sealed unsafe class NativeBuffer : MemoryManager<byte>
{
    private IntPtr address;
    private static long ownedBytes;

    public NativeBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
        address = (IntPtr)NativeMemory.AllocZeroed((nuint)Math.Max(1, length));
        if (address == IntPtr.Zero)
            throw new OutOfMemoryException();
        Interlocked.Add(ref ownedBytes, length);
    }

    public int Length { get; }
    public static long OwnedBytes => Interlocked.Read(ref ownedBytes);

    public IntPtr Address
    {
        get
        {
            IntPtr current = Volatile.Read(ref address);
            ObjectDisposedException.ThrowIf(current == IntPtr.Zero, this);
            return current;
        }
    }

    public override Span<byte> GetSpan() => new((void*)Address, Length);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, (uint)Length);
        return new MemoryHandle((byte*)Address + elementIndex);
    }

    public override void Unpin() { }

    public UnmanagedMemoryStream OpenRead() => new((byte*)Address, Length);

    public void Dispose() => Dispose(true);

    protected override void Dispose(bool disposing)
    {
        IntPtr released = Interlocked.Exchange(ref address, IntPtr.Zero);
        if (released == IntPtr.Zero)
            return;
        NativeMemory.Free((void*)released);
        Interlocked.Add(ref ownedBytes, -Length);
    }
}